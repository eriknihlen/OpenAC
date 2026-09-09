using AcDream.App.Rendering.Wb;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class AnimatedEntityStateTests
{
    [Fact]
    public void DefaultState_HasNoOverridesAndNoHiddenParts()
    {
        var state = MakeState();

        Assert.False(state.IsPartHidden(0));
        Assert.False(state.IsPartHidden(63));
        Assert.False(state.TryGetPartOverride(0, out _));
    }

    [Fact]
    public void Sequencer_AccessibleAsProperty()
    {
        var sequencer = MakeSequencer();
        var state = new AnimatedEntityState(sequencer);

        Assert.Same(sequencer, state.Sequencer);
    }

    [Fact]
    public void Construct_WithNullSequencer_ThrowsArgumentNull()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => new AnimatedEntityState(null!));
    }

    private static AnimatedEntityState MakeState() => new(MakeSequencer());

    private static AnimationSequencer MakeSequencer()
        => new AnimationSequencer(new Setup(), new MotionTable(), new NullAnimationLoader());

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
