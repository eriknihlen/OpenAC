using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

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

    public static MotionData MakeMotionData(uint animId, float framerate, byte bitfield = 0,
        Vector3? velocity = null, Vector3? omega = null)
    {
        var md = new MotionData { Bitfield = bitfield };
        md.Anims.Add(MakeAnimData(animId, framerate));
        if (velocity is { } v)
        {
            md.Velocity = v;
            md.Flags |= MotionDataFlags.HasVelocity;
        }
        if (omega is { } w)
        {
            md.Omega = w;
            md.Flags |= MotionDataFlags.HasOmega;
        }
        return md;
    }

    /// <summary>MotionData with N AnimData entries (for outTicks sum tests, A3).</summary>
    public static MotionData MakeMultiAnimMotionData(uint firstAnimId, int count, float framerate)
    {
        var md = new MotionData();
        for (int i = 0; i < count; i++)
            md.Anims.Add(MakeAnimData(firstAnimId + (uint)i, framerate));
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

    public static void AddModifier(MotionTable mt, uint style, uint modifier, MotionData data, bool styleSpecific = true)
    {
        int key = styleSpecific ? (int)((style << 16) | (modifier & 0xFFFFFFu)) : (int)(modifier & 0xFFFFFFu);
        mt.Modifiers[key] = data;
    }
}

public sealed class CMotionTableTests
{
    private const uint Ready = 0x41000003u;
    private const uint WalkForward = 0x45000005u;
    private const uint RunForward = 0x44000007u;
    private const uint TurnRight = 0x6500000Du;
    private const uint Jump = 0x2500003Bu; // modifier only
    private const uint ThrustMed = 0x10000058u; // action only

    private const uint HandCombatStyle = 0x8000003Cu; // style, top bit set
    private const uint NonCombatStyle = 0x8000003Du;  // style, top bit set


    [Fact]
    public void GetObjectSequence_ReadyToWalk_BuildsLinkThenCycleChain()
    {
        var loader = new FakeLoader();
        uint readyAnim = 0x03000001u, walkAnim = 0x03000002u, linkAnim = 0x03000003u;
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(walkAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(linkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;

        var readyCycle = Fixtures.MakeMotionData(readyAnim, 30f);
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, readyCycle);

        var walkCycle = Fixtures.MakeMotionData(walkAnim, 30f);
        Fixtures.AddCycle(mt, NonCombatStyle, WalkForward, walkCycle);

        var link = Fixtures.MakeMotionData(linkAnim, 30f);
        Fixtures.AddLink(mt, NonCombatStyle, Ready, WalkForward, link);

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(loader);

        bool ok = cmt.GetObjectSequence(WalkForward, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(WalkForward, state.Substate);
        Assert.Equal(NonCombatStyle, state.Style);
        Assert.Equal(1f, state.SubstateMod);
        Assert.Equal(2, seq.Count);
        Assert.Equal(1u, outTicks);
    }

    [Fact]
    public void GetObjectSequence_ReadyToWalk_FirstCyclicIsTheCycleNotTheLink()
    {
        var loader = new FakeLoader();
        uint linkAnim = 0x03000010u, cycleAnim = 0x03000011u;
        loader.Register(linkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(cycleAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(0x03000012u, 30f));
        Fixtures.AddCycle(mt, NonCombatStyle, WalkForward, Fixtures.MakeMotionData(cycleAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, Ready, WalkForward, Fixtures.MakeMotionData(linkAnim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.GetObjectSequence(WalkForward, state, seq, 1f, out _, stopCall: false));

        Assert.NotNull(seq.FirstCyclic);
        Assert.NotNull(seq.CurrAnim);
        Assert.Equal(2, seq.Count);
    }

    // ── walk↔run same-substate re-speed fast path (H8) ─────────────────

    [Fact]
    public void GetObjectSequence_SameSubstateSameSignFasterSpeed_TakesReSpeedFastPath_NoListChange()
    {
        var loader = new FakeLoader();
        uint walkAnim = 0x03000020u;
        loader.Register(walkAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        var walkCycle = Fixtures.MakeMotionData(walkAnim, 30f, velocity: new Vector3(0, 3.12f, 0));
        Fixtures.AddCycle(mt, NonCombatStyle, WalkForward, walkCycle);

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = WalkForward, SubstateMod = 1f };
        var seq = new CSequence(loader);

        // Seed the sequence as if WalkForward is already playing at speed 1.0.
        Assert.True(cmt.DoObjectMotion(WalkForward, state, seq, 1f, out _));
        int countBefore = seq.Count;
        var firstCyclicBefore = seq.FirstCyclic;
        var velBefore = seq.Velocity;

        bool ok = cmt.GetObjectSequence(WalkForward, state, seq, 2f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(2f, state.SubstateMod);
        Assert.Equal(countBefore, seq.Count);
        Assert.Same(firstCyclicBefore, seq.FirstCyclic);
        Assert.Equal(velBefore * 2f, seq.Velocity);
        Assert.Equal(0u, outTicks);
    }

    [Fact]
    public void GetObjectSequence_SameSubstateSameSignFastPath_RescalesFramerates()
    {
        var loader = new FakeLoader();
        uint walkAnim = 0x03000021u;
        loader.Register(walkAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, WalkForward, Fixtures.MakeMotionData(walkAnim, 10f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = WalkForward, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.DoObjectMotion(WalkForward, state, seq, 1f, out _));
        double frBefore = seq.CurrAnim!.Framerate;

        Assert.True(cmt.GetObjectSequence(WalkForward, state, seq, 2.5f, out _, stopCall: false));

        double frAfter = seq.CurrAnim!.Framerate;
        Assert.Equal(frBefore * 2.5, frAfter, 3);
    }

    // ── sign-flip Walk(+)→Walk(-) routes the style-default double-hop (A2) ──

    [Fact]
    public void GetObjectSequence_SignFlipSameSubstate_RoutesStyleDefaultDoubleHop()
    {
        // Walk(+1.0) -> Walk(-1.0): same substate id, OPPOSITE sign. This must
        // NOT take the re-speed fast path (same_sign gate fails) and must NOT
        // take the direct-link path (no link registered substate->substate);
        // it routes via the style-default double-hop:
        //   hop1: get_link(style, substate, oldSpeed, styleDefaultSubstate, 1f)
        //   hop2: get_link(style, styleDefaultSubstate, 1f, substate, newSpeed)
        var loader = new FakeLoader();
        uint walkAnim = 0x03000030u, hop1Anim = 0x03000031u, hop2Anim = 0x03000032u;
        loader.Register(walkAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(hop1Anim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(hop2Anim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, WalkForward, Fixtures.MakeMotionData(walkAnim, 30f));
        // hop1: WalkForward -> Ready (style default), hop2: Ready -> WalkForward
        Fixtures.AddLink(mt, NonCombatStyle, WalkForward, Ready, Fixtures.MakeMotionData(hop1Anim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, Ready, WalkForward, Fixtures.MakeMotionData(hop2Anim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = WalkForward, SubstateMod = 1f };
        var seq = new CSequence(loader);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(walkAnim, 30f), 1f);
        Assert.Equal(1, seq.Count);

        bool ok = cmt.GetObjectSequence(WalkForward, state, seq, -1f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(-1f, state.SubstateMod);
        Assert.Equal(WalkForward, state.Substate);
        Assert.Equal(3, seq.Count);
    }


    [Fact]
    public void GetObjectSequence_StyleChange_BuildsExitLinkPlusStyleLinkPlusNewCycle()
    {
        var loader = new FakeLoader();
        uint exitAnim = 0x03000040u, styleLinkAnim = 0x03000041u, newCycleAnim = 0x03000042u;
        loader.Register(exitAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(styleLinkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(newCycleAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        mt.StyleDefaults[(DRWMotionCommand)HandCombatStyle] = (DRWMotionCommand)Ready;

        Fixtures.AddCycle(mt, NonCombatStyle, WalkForward, Fixtures.MakeMotionData(0x03000043u, 30f));
        // exit-link: WalkForward -> NonCombat's default (Ready)
        Fixtures.AddLink(mt, NonCombatStyle, WalkForward, Ready, Fixtures.MakeMotionData(exitAnim, 30f));
        // style-to-style link: NonCombat's default (Ready) -> HandCombat's default (Ready)
        Fixtures.AddLink(mt, NonCombatStyle, Ready, HandCombatStyle, Fixtures.MakeMotionData(styleLinkAnim, 30f));
        Fixtures.AddCycle(mt, HandCombatStyle, Ready, Fixtures.MakeMotionData(newCycleAnim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = WalkForward, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.DoObjectMotion(WalkForward, state, seq, 1f, out _));

        bool ok = cmt.GetObjectSequence(HandCombatStyle, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(HandCombatStyle, state.Style);
        Assert.Equal(Ready, state.Substate);
        Assert.Equal(3, seq.Count);
    }

    [Fact]
    public void GetObjectSequence_StyleChange_AlreadyInTargetStyle_IsNoOp()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(new NullLoader());

        bool ok = cmt.GetObjectSequence(NonCombatStyle, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(0u, outTicks);
        Assert.Equal(0, seq.Count); // untouched
    }


    [Fact]
    public void GetObjectSequence_ActionDirectLink_QueuesActionAndReAddsBaseCycle()
    {
        var loader = new FakeLoader();
        uint cycleAnim = 0x03000050u, actionAnim = 0x03000051u;
        loader.Register(cycleAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(actionAnim, Fixtures.MakeAnim(3, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, RunForward, Fixtures.MakeMotionData(cycleAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, RunForward, ThrustMed, Fixtures.MakeMotionData(actionAnim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.DoObjectMotion(RunForward, state, seq, 1f, out _));

        bool ok = cmt.GetObjectSequence(ThrustMed, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(2, seq.Count);
        // Action queued onto the FIFO.
        Assert.Single(state.Actions);
        Assert.Equal(ThrustMed, state.Actions.First().Motion);
        Assert.Equal(RunForward, state.Substate);
        // A3: outTicks = action's num_anims (1) only, direct-link path.
        Assert.Equal(1u, outTicks);
    }

    [Fact]
    public void GetObjectSequence_ActionNoDirectLink_FourLayerOutAndBack_BaseCycleAtOldSubstateMod()
    {
        var loader = new FakeLoader();
        uint cycleAnim = 0x03000060u, outHopAnim = 0x03000061u, actionAnim = 0x03000062u, returnHopAnim = 0x03000063u;
        loader.Register(cycleAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(outHopAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(actionAnim, Fixtures.MakeAnim(3, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(returnHopAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, RunForward, Fixtures.MakeMotionData(cycleAnim, 30f));
        // NO direct RunForward -> ThrustMed link. Route via style default (Ready).
        Fixtures.AddLink(mt, NonCombatStyle, RunForward, Ready, Fixtures.MakeMotionData(outHopAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, Ready, ThrustMed, Fixtures.MakeMotionData(actionAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, Ready, RunForward, Fixtures.MakeMotionData(returnHopAnim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward, SubstateMod = 1.5f };
        var seq = new CSequence(loader);
        CMotionTable.AddMotion(seq, Fixtures.MakeMotionData(cycleAnim, 30f), 1.5f);
        Assert.Equal(1, seq.Count);

        bool ok = cmt.GetObjectSequence(ThrustMed, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(4, seq.Count);
        Assert.Equal(RunForward, state.Substate);
        Assert.Equal(1.5f, state.SubstateMod);

        Assert.NotNull(seq.FirstCyclic);
        Assert.Equal(45.0, seq.FirstCyclic!.Framerate, 3);

        Assert.Equal(3u, outTicks);
    }


    [Fact]
    public void GetObjectSequence_TurnInPlace_ResolvesAsCycleFromReady()
    {
        var loader = new FakeLoader();
        uint readyAnim = 0x03000070u, turnAnim = 0x03000071u, linkAnim = 0x03000072u;
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(turnAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(linkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));
        Fixtures.AddCycle(mt, NonCombatStyle, TurnRight, Fixtures.MakeMotionData(turnAnim, 30f, bitfield: 0));
        Fixtures.AddLink(mt, NonCombatStyle, Ready, TurnRight, Fixtures.MakeMotionData(linkAnim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(loader);

        bool ok = cmt.GetObjectSequence(TurnRight, state, seq, 1f, out _, stopCall: false);

        Assert.True(ok);
        Assert.Equal(TurnRight, state.Substate);
        Assert.Equal(2, seq.Count);
    }


    [Fact]
    public void GetObjectSequence_RunWhileTurning_GatedTurnCycleRejected_FallsToModifierPhysicsOnlyCombine()
    {
        var loader = new FakeLoader();
        uint runAnim = 0x03000080u, turnAnim = 0x03000081u;
        loader.Register(runAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(turnAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;

        var runVelocity = new Vector3(0, 4.0f, 0);
        Fixtures.AddCycle(mt, NonCombatStyle, RunForward, Fixtures.MakeMotionData(runAnim, 30f, velocity: runVelocity));

        Fixtures.AddCycle(mt, NonCombatStyle, TurnRight, Fixtures.MakeMotionData(turnAnim, 30f, bitfield: 2));
        // TurnRight AS A MODIFIER: physics-only turn omega, resolved from the
        // Modifiers dict once Branch 2 falls through.
        var turnOmega = new Vector3(0, 0, -(MathF.PI / 2f));
        Fixtures.AddModifier(mt, NonCombatStyle, TurnRight, Fixtures.MakeMotionData(0, 0f, omega: turnOmega));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.DoObjectMotion(RunForward, state, seq, 1f, out _));
        int countBefore = seq.Count;
        var cyclicBefore = seq.FirstCyclic;
        var currBefore = seq.CurrAnim;

        bool ok = cmt.GetObjectSequence(TurnRight, state, seq, 1f, out _, stopCall: false);

        Assert.True(ok);
        // Run anims UNTOUCHED — Branch 4 is physics-only, no add/remove of anim nodes.
        Assert.Equal(countBefore, seq.Count);
        Assert.Equal(cyclicBefore, seq.FirstCyclic);
        Assert.Equal(currBefore, seq.CurrAnim);
        Assert.Equal(RunForward, state.Substate);
        // TurnRight now tracked as an active modifier.
        Assert.Contains(state.Modifiers, m => m.Motion == TurnRight);
        Assert.Equal(runVelocity, seq.Velocity);
        Assert.Equal(turnOmega, seq.Omega);
    }

    // ── modifier stop = subtract + unlink ───────────────────────────────

    [Fact]
    public void StopSequenceMotion_Modifier_SubtractsPhysicsAndUnlinksFromModifierChain()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        var jumpOmega = new Vector3(0, 0, 5f);
        Fixtures.AddModifier(mt, NonCombatStyle, Jump, Fixtures.MakeMotionData(0, 0f, omega: jumpOmega));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(new NullLoader());

        // Activate the modifier directly (bypassing GetObjectSequence's gate logic).
        state.AddModifierNoCheck(Jump, 1f);
        seq.CombinePhysics(Vector3.Zero, jumpOmega);
        Assert.Equal(jumpOmega, seq.Omega);

        bool ok = cmt.StopSequenceMotion(Jump, 1f, state, seq, out uint outTicks);

        Assert.True(ok);
        Assert.Equal(Vector3.Zero, seq.Omega); // subtracted back out
        Assert.DoesNotContain(state.Modifiers, m => m.Motion == Jump); // unlinked
        Assert.Equal(0u, outTicks);
    }

    [Fact]
    public void StopSequenceMotion_UnknownModifier_ReturnsFalse_NoOp()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(new NullLoader());

        bool ok = cmt.StopSequenceMotion(Jump, 1f, state, seq, out uint outTicks);

        Assert.False(ok);
        Assert.Equal(0u, outTicks);
    }


    [Fact]
    public void StopObjectCompletely_DrainsAllModifiers_ThenRedrivesToStyleDefault()
    {
        var loader = new FakeLoader();
        uint runAnim = 0x03000090u, readyAnim = 0x03000091u, exitLinkAnim = 0x03000092u;
        loader.Register(runAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(exitLinkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, RunForward, Fixtures.MakeMotionData(runAnim, 30f));
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));
        Fixtures.AddLink(mt, NonCombatStyle, RunForward, Ready, Fixtures.MakeMotionData(exitLinkAnim, 30f));

        var jumpOmega = new Vector3(1, 0, 0);
        Fixtures.AddModifier(mt, NonCombatStyle, Jump, Fixtures.MakeMotionData(0, 0f, omega: jumpOmega));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.DoObjectMotion(RunForward, state, seq, 1f, out _));
        state.AddModifierNoCheck(Jump, 1f);
        seq.CombinePhysics(Vector3.Zero, jumpOmega);

        bool ok = cmt.StopObjectCompletely(state, seq, out _);

        Assert.True(ok);
        Assert.Empty(state.Modifiers); // drained
        Assert.Equal(Ready, state.Substate); // re-driven to style default
        Assert.Equal(Vector3.Zero, seq.Omega);
    }


    [Fact]
    public void GetObjectSequence_MissingCycle_ReturnsFalse_SequenceUntouched()
    {
        var loader = new FakeLoader();
        uint readyAnim = 0x030000A0u;
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.DoObjectMotion(Ready, state, seq, 1f, out _));
        int countBefore = seq.Count;
        var velBefore = seq.Velocity;
        var omegaBefore = seq.Omega;
        uint substateBefore = state.Substate;

        bool ok = cmt.GetObjectSequence(RunForward, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.False(ok);
        Assert.Equal(0u, outTicks);
        Assert.Equal(countBefore, seq.Count); // list untouched
        Assert.Equal(velBefore, seq.Velocity);
        Assert.Equal(omegaBefore, seq.Omega);
        Assert.Equal(substateBefore, state.Substate); // state untouched
    }

    // ── entry guards ─────────────────────────────────────────────────────

    [Fact]
    public void GetObjectSequence_ZeroStyle_ReturnsFalse()
    {
        var mt = new MotionTable();
        var cmt = new CMotionTable(mt);
        var state = new MotionState();
        var seq = new CSequence(new NullLoader());

        bool ok = cmt.GetObjectSequence(WalkForward, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.False(ok);
        Assert.Equal(0u, outTicks);
    }

    [Fact]
    public void GetObjectSequence_ZeroSubstate_ReturnsFalse()
    {
        var mt = new MotionTable();
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = 0 };
        var seq = new CSequence(new NullLoader());

        bool ok = cmt.GetObjectSequence(WalkForward, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.False(ok);
        Assert.Equal(0u, outTicks);
    }


    [Fact]
    public void GetObjectSequence_ModifierClassTargetEqualsStyleDefault_NotStopCall_IsNoOp()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Jump;

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Jump, SubstateMod = 1f };
        var seq = new CSequence(new NullLoader());

        bool ok = cmt.GetObjectSequence(Jump, state, seq, 1f, out uint outTicks, stopCall: false);

        Assert.True(ok);
        Assert.Equal(0u, outTicks);
        Assert.Equal(0, seq.Count); // no-op, nothing built
    }


    [Fact]
    public void ReModify_ReplaysActiveModifiers_ThroughGetObjectSequence()
    {
        var loader = new FakeLoader();
        uint runAnim = 0x030000B0u;
        loader.Register(runAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, RunForward, Fixtures.MakeMotionData(runAnim, 30f));
        var jumpOmega = new Vector3(0, 0, 9f);
        Fixtures.AddModifier(mt, NonCombatStyle, Jump, Fixtures.MakeMotionData(0, 0f, omega: jumpOmega));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward, SubstateMod = 1f };
        var seq = new CSequence(loader);

        Assert.True(cmt.DoObjectMotion(RunForward, state, seq, 1f, out _));

        state.AddModifierNoCheck(Jump, 1f);
        seq.ClearPhysics(); // simulate a fresh rebuild wiping physics

        cmt.ReModify(seq, state);

        Assert.Equal(jumpOmega, seq.Omega);
        Assert.Contains(state.Modifiers, m => m.Motion == Jump);
    }

    [Fact]
    public void ReModify_EmptyModifierChain_IsNoOp()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(new NullLoader());

        cmt.ReModify(seq, state); // must not throw; no modifiers to replay

        Assert.Empty(state.Modifiers);
        Assert.Equal(0, seq.Count);
    }


    [Theory]
    [InlineData(1f, 1f, true)]
    [InlineData(-1f, -1f, true)]
    [InlineData(1f, -1f, false)]
    [InlineData(-1f, 1f, false)]
    [InlineData(0f, 1f, true)]
    [InlineData(0f, -1f, false)]
    public void SameSign_MatchesRetailSemantics(float a, float b, bool expected)
    {
        Assert.Equal(expected, CMotionTable.SameSign(a, b));
    }

    [Fact]
    public void ChangeCycleSpeed_RescalesFramerate_ByRatio()
    {
        var loader = new FakeLoader();
        uint animId = 0x030000C0u;
        loader.Register(animId, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Fixtures.MakeAnimData(animId, 10f));

        var md = Fixtures.MakeMotionData(animId, 10f);
        CMotionTable.ChangeCycleSpeed(seq, md, oldSpeed: 1f, newSpeed: 2f);

        Assert.Equal(20.0, seq.CurrAnim!.Framerate, 3);
    }

    [Fact]
    public void ChangeCycleSpeed_OldSpeedNearZero_NewSpeedNearZero_ZeroesFramerate()
    {
        var loader = new FakeLoader();
        uint animId = 0x030000C1u;
        loader.Register(animId, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Fixtures.MakeAnimData(animId, 10f));

        var md = Fixtures.MakeMotionData(animId, 10f);
        CMotionTable.ChangeCycleSpeed(seq, md, oldSpeed: 0.0001f, newSpeed: 0.0001f);

        Assert.Equal(0.0, seq.CurrAnim!.Framerate, 5);
    }

    [Fact]
    public void ChangeCycleSpeed_OldSpeedNearZero_NewSpeedNonZero_SilentNoOp_A4Gap()
    {
        var loader = new FakeLoader();
        uint animId = 0x030000C2u;
        loader.Register(animId, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        var seq = new CSequence(loader);
        seq.AppendAnimation(Fixtures.MakeAnimData(animId, 10f));

        var md = Fixtures.MakeMotionData(animId, 10f);
        CMotionTable.ChangeCycleSpeed(seq, md, oldSpeed: 0.0001f, newSpeed: 5f);

        Assert.Equal(10.0, seq.CurrAnim!.Framerate, 5);
    }

    [Fact]
    public void AddMotion_NullMotionData_IsNoOp()
    {
        var seq = new CSequence(new NullLoader());
        CMotionTable.AddMotion(seq, null, 1f);
        Assert.Equal(0, seq.Count);
        Assert.Equal(Vector3.Zero, seq.Velocity);
    }

    [Fact]
    public void AddMotion_SetsVelocityOmega_Unconditionally_EvenWhenZero()
    {
        var loader = new FakeLoader();
        uint animId = 0x030000D0u;
        loader.Register(animId, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));
        var seq = new CSequence(loader);
        seq.SetVelocity(new Vector3(9, 9, 9));

        var md = Fixtures.MakeMotionData(animId, 10f); // no HasVelocity flag -> Velocity field is default(Vector3.Zero)
        CMotionTable.AddMotion(seq, md, 1f);

        Assert.Equal(Vector3.Zero, seq.Velocity); // overwritten with zero, not left at (9,9,9)
    }

    [Fact]
    public void CombineMotion_AddsToExistingPhysics_AnimsUntouched()
    {
        var seq = new CSequence(new NullLoader());
        seq.SetVelocity(new Vector3(1, 0, 0));
        var md = Fixtures.MakeMotionData(0, 0f, velocity: new Vector3(2, 0, 0));

        CMotionTable.CombineMotion(seq, md, 1f);

        Assert.Equal(new Vector3(3, 0, 0), seq.Velocity);
        Assert.Equal(0, seq.Count);
    }

    [Fact]
    public void SubtractMotion_RemovesFromExistingPhysics_AnimsUntouched()
    {
        var seq = new CSequence(new NullLoader());
        seq.SetVelocity(new Vector3(5, 0, 0));
        var md = Fixtures.MakeMotionData(0, 0f, velocity: new Vector3(2, 0, 0));

        CMotionTable.SubtractMotion(seq, md, 1f);

        Assert.Equal(new Vector3(3, 0, 0), seq.Velocity);
        Assert.Equal(0, seq.Count);
    }


    [Fact]
    public void GetLink_ForwardDirection_LooksUpBySubstateThenMotion()
    {
        var mt = new MotionTable();
        var link = Fixtures.MakeMotionData(0x030000E0u, 30f);
        Fixtures.AddLink(mt, NonCombatStyle, Ready, WalkForward, link);
        var cmt = new CMotionTable(mt);

        var result = cmt.GetLink(NonCombatStyle, Ready, 1f, WalkForward, 1f);

        Assert.Same(link, result);
    }

    [Fact]
    public void GetLink_ReversedDirection_EitherSpeedNegative_SwapsKeys()
    {
        // A1 pin: EITHER speed negative -> swapped-key branch (link stored
        // FROM motion TO substate).
        var mt = new MotionTable();
        var reversedLink = Fixtures.MakeMotionData(0x030000E1u, 30f);
        Fixtures.AddLink(mt, NonCombatStyle, WalkForward, Ready, reversedLink);
        var cmt = new CMotionTable(mt);

        // fromSubstate=Ready(+1), toSubstate=WalkForward but NEGATIVE speed.
        var result = cmt.GetLink(NonCombatStyle, Ready, 1f, WalkForward, -1f);

        Assert.Same(reversedLink, result);
    }

    [Fact]
    public void IsAllowed_UngatedMotionData_AlwaysAllowed()
    {
        var mt = new MotionTable();
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready };
        var md = Fixtures.MakeMotionData(0x030000F0u, 30f, bitfield: 0);

        Assert.True(cmt.IsAllowed(WalkForward, md, state));
    }

    [Fact]
    public void IsAllowed_GatedMotionData_CandidateEqualsCurrentSubstate_Allowed()
    {
        var mt = new MotionTable();
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = TurnRight };
        var md = Fixtures.MakeMotionData(0x030000F1u, 30f, bitfield: 2);

        Assert.True(cmt.IsAllowed(TurnRight, md, state));
    }

    [Fact]
    public void IsAllowed_GatedMotionData_CurrentSubstateIsStyleDefault_Allowed()
    {
        var mt = new MotionTable();
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready };
        var md = Fixtures.MakeMotionData(0x030000F2u, 30f, bitfield: 2);

        Assert.True(cmt.IsAllowed(TurnRight, md, state));
    }

    [Fact]
    public void IsAllowed_GatedMotionData_CurrentSubstateMismatch_Rejected()
    {
        var mt = new MotionTable();
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = RunForward }; // neither TurnRight nor Ready
        var md = Fixtures.MakeMotionData(0x030000F3u, 30f, bitfield: 2);

        Assert.False(cmt.IsAllowed(TurnRight, md, state));
    }

    [Fact]
    public void IsAllowed_NullMotionData_Rejected()
    {
        var mt = new MotionTable();
        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready };

        Assert.False(cmt.IsAllowed(TurnRight, null, state));
    }

    // ── SetDefaultState (hard reset) ─────────────────────────────────────

    [Fact]
    public void SetDefaultState_ResetsToTableDefaultStyleAndSubstate_ClearAnimationsHardReset()
    {
        var loader = new FakeLoader();
        uint readyAnim = 0x03000100u, junkAnim = 0x03000101u;
        loader.Register(readyAnim, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));
        loader.Register(junkAnim, Fixtures.MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(readyAnim, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = HandCombatStyle, Substate = RunForward, SubstateMod = 3f };
        state.AddModifierNoCheck(Jump, 1f);
        state.AddAction(ThrustMed, 1f);

        var seq = new CSequence(loader);
        seq.AppendAnimation(Fixtures.MakeAnimData(junkAnim, 5f)); // stale junk node

        bool ok = cmt.SetDefaultState(state, seq, out uint outTicks);

        Assert.True(ok);
        Assert.Equal(NonCombatStyle, state.Style);
        Assert.Equal(Ready, state.Substate);
        Assert.Equal(1f, state.SubstateMod);
        Assert.Empty(state.Modifiers);
        Assert.Empty(state.Actions);
        Assert.Equal(1, seq.Count);
        Assert.Equal(0u, outTicks);
    }

    [Fact]
    public void SetDefaultState_TableHasNoDefaultStyleEntry_ReturnsFalse()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        // No StyleDefaults entry registered for NonCombatStyle.
        var cmt = new CMotionTable(mt);
        var state = new MotionState();
        var seq = new CSequence(new NullLoader());

        bool ok = cmt.SetDefaultState(state, seq, out uint outTicks);

        Assert.False(ok);
        Assert.Equal(0u, outTicks);
    }


    [Fact]
    public void DoObjectMotion_ForcesStopFlagFalse_DelegatesToGetObjectSequence()
    {
        var loader = new FakeLoader();
        uint animId = 0x03000110u;
        loader.Register(animId, Fixtures.MakeAnim(4, 1, Vector3.Zero, Quaternion.Identity));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        mt.StyleDefaults[(DRWMotionCommand)NonCombatStyle] = (DRWMotionCommand)Ready;
        Fixtures.AddCycle(mt, NonCombatStyle, Ready, Fixtures.MakeMotionData(animId, 30f));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        var seq = new CSequence(loader);

        bool ok = cmt.DoObjectMotion(Ready, state, seq, 1f, out _);

        Assert.True(ok);
        Assert.Equal(1, seq.Count);
    }

    [Fact]
    public void StopObjectMotion_DelegatesToStopSequenceMotion()
    {
        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombatStyle };
        var jumpOmega = new Vector3(0, 0, 3f);
        Fixtures.AddModifier(mt, NonCombatStyle, Jump, Fixtures.MakeMotionData(0, 0f, omega: jumpOmega));

        var cmt = new CMotionTable(mt);
        var state = new MotionState { Style = NonCombatStyle, Substate = Ready, SubstateMod = 1f };
        state.AddModifierNoCheck(Jump, 1f);
        var seq = new CSequence(new NullLoader());
        seq.CombinePhysics(Vector3.Zero, jumpOmega);

        bool ok = cmt.StopObjectMotion(Jump, 1f, state, seq, out _);

        Assert.True(ok);
        Assert.Equal(Vector3.Zero, seq.Omega);
    }
}

file sealed class NullLoader : IAnimationLoader
{
    public Animation? LoadAnimation(uint id) => null;
}

file sealed class FakeLoader : IAnimationLoader
{
    private readonly System.Collections.Generic.Dictionary<uint, Animation> _anims = new();
    public void Register(uint id, Animation anim) => _anims[id] = anim;
    public Animation? LoadAnimation(uint id) => _anims.TryGetValue(id, out var a) ? a : null;
}
