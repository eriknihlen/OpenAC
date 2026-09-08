using AcDream.App.Rendering.Wb;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class AnimPartChangeTests
{
    [Fact]
    public void SetPartOverride_ResolvedAtLookup()
    {
        var state = MakeState();

        state.SetPartOverride(partIdx: 5, gfxObjId: 0x01001234ul);

        Assert.True(state.TryGetPartOverride(5, out var got));
        Assert.Equal(0x01001234ul, got);
        Assert.False(state.TryGetPartOverride(6, out _));
    }

    [Fact]
    public void SetPartOverride_TwiceForSamePart_TakesLatest()
    {
        var state = MakeState();

        state.SetPartOverride(0, 0x01000001ul);
        state.SetPartOverride(0, 0x01999999ul);

        Assert.True(state.TryGetPartOverride(0, out var got));
        Assert.Equal(0x01999999ul, got);
    }

    [Fact]
    public void ResolvePartGfxObj_WithoutOverride_ReturnsSetupDefault()
    {
        var state = MakeState();

        Assert.Equal(0x01000001ul,
            state.ResolvePartGfxObj(partIdx: 0, setupDefault: 0x01000001ul));
    }

    [Fact]
    public void ResolvePartGfxObj_WithOverride_ReturnsOverride()
    {
        var state = MakeState();
        state.SetPartOverride(partIdx: 0, gfxObjId: 0x01999999ul);

        Assert.Equal(0x01999999ul,
            state.ResolvePartGfxObj(partIdx: 0, setupDefault: 0x01000001ul));
    }

    private static AnimatedEntityState MakeState() => new(MakeSequencer());

    private static AnimationSequencer MakeSequencer()
        => new AnimationSequencer(new Setup(), new MotionTable(), new NullAnimationLoader());

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
