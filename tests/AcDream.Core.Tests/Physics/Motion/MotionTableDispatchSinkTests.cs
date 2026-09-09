using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Tests.Physics.Motion;

public class MotionTableDispatchSinkTests
{
    private const uint NC = 0x8000003Du;
    private const uint Ready = 0x41000003u;
    private const uint Walk = 0x45000005u;
    private const uint TurnRight = 0x6500000Du;

    private const uint ReadyAnim = 0x200u;
    private const uint WalkAnim = 0x201u;

    private sealed class Loader : IAnimationLoader
    {
        private readonly System.Collections.Generic.Dictionary<uint, Animation> _anims = new();
        public void Register(uint id, Animation anim) => _anims[id] = anim;
        public Animation? LoadAnimation(uint id) => _anims.TryGetValue(id, out var a) ? a : null;
    }

    private static Animation MakeAnim(int frames)
    {
        var anim = new Animation();
        for (int f = 0; f < frames; f++)
        {
            var pf = new AnimationFrame(1);
            pf.Frames.Add(new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }
        return anim;
    }

    private static MotionData MakeMd(uint animId, Vector3? omega = null)
    {
        var md = new MotionData();
        QualifiedDataId<Animation> qid = animId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = -1, Framerate = 30f });
        if (omega is { } o)
        {
            md.Omega = o;
            md.Flags |= DatReaderWriter.Enums.MotionDataFlags.HasOmega;
        }
        return md;
    }

    private static AnimationSequencer MakeSequencer(bool withTurnModifier = true)
    {
        var setup = new Setup();
        setup.Parts.Add(0x01000000u);
        setup.DefaultScale.Add(Vector3.One);

        var loader = new Loader();
        loader.Register(ReadyAnim, MakeAnim(4));
        loader.Register(WalkAnim, MakeAnim(6));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NC };
        mt.StyleDefaults[(DRWMotionCommand)NC] = (DRWMotionCommand)Ready;
        mt.Cycles[(int)((NC << 16) | (Ready & 0xFFFFFFu))] = MakeMd(ReadyAnim);
        mt.Cycles[(int)((NC << 16) | (Walk & 0xFFFFFFu))] = MakeMd(WalkAnim);
        if (withTurnModifier)
        {
            mt.Modifiers[(int)((NC << 16) | (TurnRight & 0xFFFFFFu))] =
                MakeMd(ReadyAnim, omega: new Vector3(0f, 0f, 1.2f));
        }

        return new AnimationSequencer(setup, mt, loader);
    }

    [Fact]
    public void ApplyMotion_CycleClass_InstallsSubstate_LazyInitialized()
    {
        var seq = MakeSequencer();
        var sink = new MotionTableDispatchSink(seq);

        sink.ApplyMotion(Walk, 2.0f);

        Assert.Equal(Walk, seq.CurrentMotion);
        Assert.Equal(2.0f, seq.CurrentSpeedMod);
        Assert.Equal(Vector3.Zero, seq.CurrentVelocity);
    }

    [Fact]
    public void ApplyMotion_Turn_Branch4Modifier_PreservesCycleAndAppliesDatOmega()
    {
        var seq = MakeSequencer();
        var sink = new MotionTableDispatchSink(seq);

        sink.ApplyMotion(Walk, 1.0f);
        sink.ApplyMotion(TurnRight, 1.5f);

        Assert.Equal(Walk, seq.CurrentMotion);
        Assert.Equal(1.2f * 1.5f, seq.CurrentOmega.Z, 3);
    }

    [Fact]
    public void StopMotion_Turn_UnwindsModifierPhysics()
    {
        var seq = MakeSequencer();
        var sink = new MotionTableDispatchSink(seq);

        sink.ApplyMotion(Walk, 1.0f);
        sink.ApplyMotion(TurnRight, 1.5f);
        sink.StopMotion(TurnRight);

        Assert.Equal(0f, seq.CurrentOmega.Z, 3);
    }

    [Fact]
    public void StopMotion_CurrentSubstate_ReDrivesToStyleDefault()
    {
        var seq = MakeSequencer();
        var sink = new MotionTableDispatchSink(seq);

        sink.ApplyMotion(Walk, 1.0f);
        sink.StopMotion(Walk);

        Assert.Equal(Ready, seq.CurrentMotion);
    }

    [Fact]
    public void ApplyMotion_MissingEverywhere_NoOp_DefaultKeepsPlaying()
    {
        var seq = MakeSequencer(withTurnModifier: false);
        var sink = new MotionTableDispatchSink(seq);

        sink.ApplyMotion(Walk, 1.0f);
        sink.ApplyMotion(0x44000007u, 2.0f);

        Assert.Equal(Walk, seq.CurrentMotion);
        Assert.Equal(1.0f, seq.CurrentSpeedMod);
    }
}
