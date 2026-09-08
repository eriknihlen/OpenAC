using AcDream.App.Rendering.Wb;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class HiddenPartsTests
{
    [Theory]
    [InlineData(0b0000_0000ul, 0, false)]
    [InlineData(0b0000_0001ul, 0, true)]
    [InlineData(0b1000_0000ul, 7, true)]
    [InlineData(0b1000_0000ul, 6, false)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFul, 63, true)]
    public void IsPartHidden_RespectsBitmaskBit(ulong mask, int partIdx, bool expected)
    {
        var state = MakeState();
        state.HideParts(mask);
        Assert.Equal(expected, state.IsPartHidden(partIdx));
    }

    [Fact]
    public void IsPartHidden_NegativeIdx_ReturnsFalse()
    {
        var state = MakeState();
        state.HideParts(0xFFFF_FFFF_FFFF_FFFFul);
        Assert.False(state.IsPartHidden(-1));
    }

    [Fact]
    public void IsPartHidden_PartIdxOver64_ReturnsFalse()
    {
        var state = MakeState();
        state.HideParts(0xFFFF_FFFF_FFFF_FFFFul);
        Assert.False(state.IsPartHidden(64));
    }

    [Fact]
    public void HideParts_DefaultsToNoneHidden()
    {
        var state = MakeState();
        for (int i = 0; i < 64; i++)
            Assert.False(state.IsPartHidden(i));
    }

    private static AnimatedEntityState MakeState() => new(MakeSequencer());

    private static AnimationSequencer MakeSequencer()
        => new AnimationSequencer(new Setup(), new MotionTable(), new NullAnimationLoader());

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
