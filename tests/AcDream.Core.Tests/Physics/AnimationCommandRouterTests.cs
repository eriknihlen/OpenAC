using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class AnimationCommandRouterTests
{
    private const uint NonCombat = 0x8000003Du;

    [Theory]
    [InlineData(0x00000000u, AnimationCommandRouteKind.None)]
    [InlineData(0x10000057u, AnimationCommandRouteKind.Action)]    // Sanctuary
    [InlineData(0x2500003Bu, AnimationCommandRouteKind.Modifier)]  // Jump
    [InlineData(0x13000087u, AnimationCommandRouteKind.ChatEmote)] // Wave
    [InlineData(0x41000003u, AnimationCommandRouteKind.SubState)]  // Ready
    [InlineData(0x40000011u, AnimationCommandRouteKind.SubState)]  // Dead
    [InlineData(0x8000003Du, AnimationCommandRouteKind.Ignored)]   // NonCombat style
    public void Classify_ReturnsRetailRouteKind(uint command, AnimationCommandRouteKind expected)
    {
        Assert.Equal(expected, AnimationCommandRouter.Classify(command));
    }

    [Fact]
    public void RouteWireCommand_SubState_UsesSetCycle()
    {
        var seq = MakeRoutableSequencer();

        var route = AnimationCommandRouter.RouteWireCommand(seq, NonCombat, 0x0011);

        Assert.Equal(AnimationCommandRouteKind.SubState, route);
        Assert.Equal(NonCombat, seq.CurrentStyle);
        Assert.Equal(MotionCommand.Dead, seq.CurrentMotion);
    }

    [Fact]
    public void RouteWireCommand_Sanctuary_IsActionNotDeadCycle()
    {
        var seq = MakeEmptySequencer();

        var route = AnimationCommandRouter.RouteWireCommand(seq, NonCombat, 0x0057);

        Assert.Equal(AnimationCommandRouteKind.Action, route);
        Assert.Equal(0u, seq.CurrentMotion);
    }

    [Fact]
    public void RouteWireCommand_Wave_IsChatEmote()
    {
        var seq = MakeEmptySequencer();

        var route = AnimationCommandRouter.RouteWireCommand(seq, NonCombat, 0x0087);

        Assert.Equal(AnimationCommandRouteKind.ChatEmote, route);
    }

    private static AnimationSequencer MakeEmptySequencer()
    {
        return new AnimationSequencer(new Setup(), new MotionTable(), new NullAnimationLoader());
    }

    private static AnimationSequencer MakeRoutableSequencer()
    {
        const uint Ready = 0x41000003u;
        const uint Dead = 0x40000011u;
        const uint AnimId = 0x03000001u;

        var setup = new Setup();
        setup.Parts.Add(0x01000000u);
        setup.DefaultScale.Add(System.Numerics.Vector3.One);

        var mt = new MotionTable
        {
            DefaultStyle = (DatReaderWriter.Enums.MotionCommand)NonCombat,
        };
        mt.StyleDefaults[(DatReaderWriter.Enums.MotionCommand)NonCombat] =
            (DatReaderWriter.Enums.MotionCommand)Ready;

        int ReadyKey = (int)((NonCombat << 16) | (Ready & 0xFFFFFFu));
        int DeadKey = (int)((NonCombat << 16) | (Dead & 0xFFFFFFu));
        mt.Cycles[ReadyKey] = MakeMotionData(AnimId);
        mt.Cycles[DeadKey] = MakeMotionData(AnimId);

        var loader = new NullAnimationLoader();
        loader.Register(AnimId, MakeAnim());

        return new AnimationSequencer(setup, mt, loader);
    }

    private static MotionData MakeMotionData(uint animId)
    {
        var md = new MotionData();
        QualifiedDataId<Animation> qid = animId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = -1, Framerate = 30f });
        return md;
    }

    private static Animation MakeAnim()
    {
        var anim = new Animation();
        var pf = new AnimationFrame(1);
        pf.Frames.Add(new Frame
        {
            Origin = System.Numerics.Vector3.Zero,
            Orientation = System.Numerics.Quaternion.Identity,
        });
        anim.PartFrames.Add(pf);
        return anim;
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        private readonly System.Collections.Generic.Dictionary<uint, Animation> _anims = new();
        public void Register(uint id, Animation anim) => _anims[id] = anim;
        public Animation? LoadAnimation(uint id) => _anims.TryGetValue(id, out var a) ? a : null;
    }
}
