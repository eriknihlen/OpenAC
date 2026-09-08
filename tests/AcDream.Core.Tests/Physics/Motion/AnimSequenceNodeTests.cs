using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics.Motion;

public class AnimSequenceNodeTests
{
    private sealed class FakeLoader : IAnimationLoader
    {
        private readonly Animation? _anim;
        public FakeLoader(Animation? anim) => _anim = anim;
        public Animation? LoadAnimation(uint id) => _anim;
    }

    private static Animation MakeAnim(int numFrames, bool posFrames = false)
    {
        var anim = new Animation();
        for (int f = 0; f < numFrames; f++)
        {
            var pf = new AnimationFrame(1u);
            pf.Frames.Add(new Frame { Origin = new Vector3(f, 0, 0), Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
            if (posFrames)
                anim.PosFrames.Add(new Frame { Origin = new Vector3(0, f, 0), Orientation = Quaternion.Identity });
        }
        return anim;
    }

    [Fact]
    public void DefaultCtor_RetailDefaults()
    {
        var n = new AnimSequenceNode();
        Assert.Equal(30f, n.Framerate);
        Assert.Equal(-1, n.LowFrame);
        Assert.Equal(-1, n.HighFrame);
        Assert.False(n.HasAnim);
    }

    [Fact]
    public void SetAnimationId_Zero_LeavesAnimNullAndFramesUnclamped()
    {
        var n = new AnimSequenceNode();
        n.SetAnimationId(0, new FakeLoader(MakeAnim(10)));
        Assert.False(n.HasAnim);
        Assert.Equal(-1, n.LowFrame);
        Assert.Equal(-1, n.HighFrame);
    }

    [Theory]
    [InlineData(0, -1, 10, 0, 9)]     // high<0 → num-1 ("play to end")
    [InlineData(12, -1, 10, 9, 9)]    // high<0 first, then low>=num → num-1
    [InlineData(3, 15, 10, 3, 9)]     // high>=num → num-1
    [InlineData(15, 15, 10, 9, 9)]
    [InlineData(5, 2, 10, 5, 5)]
    [InlineData(2, 7, 10, 2, 7)]      // in-range untouched
    public void SetAnimationId_ClampOrder(int low, int high, int numFrames, int expLow, int expHigh)
    {
        var n = new AnimSequenceNode { LowFrame = low, HighFrame = high };
        n.SetAnimationId(0x0300ABCDu, new FakeLoader(MakeAnim(numFrames)));
        Assert.True(n.HasAnim);
        Assert.Equal(expLow, n.LowFrame);
        Assert.Equal(expHigh, n.HighFrame);
    }

    [Theory]
    // framerate, low, high, expectedStart, expectedEnd — BARE INTS, no epsilon
    [InlineData(30f, 2, 7, 2, 8)]     // forward: start=low, end=high+1
    [InlineData(-30f, 2, 7, 8, 2)]    // reverse: start=high+1, end=low
    [InlineData(0f, 2, 7, 2, 8)]      // zero framerate is NOT < 0 → forward
    [InlineData(30f, 0, 0, 0, 1)]     // single-frame forward
    [InlineData(-30f, 0, 0, 1, 0)]    // single-frame reverse
    public void BoundaryPair_DirectionAware_BareInts(
        float framerate, int low, int high, int expStart, int expEnd)
    {
        var n = new AnimSequenceNode { Framerate = framerate, LowFrame = low, HighFrame = high };
        Assert.Equal(expStart, n.GetStartingFrame());
        Assert.Equal(expEnd, n.GetEndingFrame());
    }

    [Fact]
    public void MultiplyFramerate_Positive_NoSwap()
    {
        var n = new AnimSequenceNode { Framerate = 30f, LowFrame = 2, HighFrame = 7 };
        n.MultiplyFramerate(2f);
        Assert.Equal(60f, n.Framerate);
        Assert.Equal(2, n.LowFrame);
        Assert.Equal(7, n.HighFrame);
    }

    [Fact]
    public void MultiplyFramerate_Negative_SwapsLowHigh()
    {
        var n = new AnimSequenceNode { Framerate = 30f, LowFrame = 2, HighFrame = 7 };
        n.MultiplyFramerate(-1f);
        Assert.Equal(-30f, n.Framerate);
        Assert.Equal(7, n.LowFrame);
        Assert.Equal(2, n.HighFrame);
        // Reverse playback boundaries against the SWAPPED fields:
        // start = high_frame(2)+1 = 3?? — no: framerate<0 → start = high+1
        // where high_frame is now 2 → 3; end = low_frame = 7.
        Assert.Equal(3, n.GetStartingFrame());
        Assert.Equal(7, n.GetEndingFrame());
    }

    [Fact]
    public void MultiplyFramerate_DoubleNegative_RoundTrips()
    {
        var n = new AnimSequenceNode { Framerate = 30f, LowFrame = 2, HighFrame = 7 };
        n.MultiplyFramerate(-1f);
        n.MultiplyFramerate(-1f);
        Assert.Equal(30f, n.Framerate);
        Assert.Equal(2, n.LowFrame);
        Assert.Equal(7, n.HighFrame);
    }

    [Fact]
    public void GetPosFrame_NullAnim_ReturnsNull()
    {
        var n = new AnimSequenceNode();
        Assert.Null(n.GetPosFrame(0));
    }

    [Fact]
    public void GetPosFrame_OutOfRange_ReturnsNull()
    {
        var n = new AnimSequenceNode();
        n.SetAnimationId(1, new FakeLoader(MakeAnim(3, posFrames: true)));
        Assert.Null(n.GetPosFrame(-1));
        Assert.Null(n.GetPosFrame(3));
        Assert.NotNull(n.GetPosFrame(2));
    }

    [Fact]
    public void GetPosFrame_DoubleOverload_Floors()
    {
        var n = new AnimSequenceNode();
        n.SetAnimationId(1, new FakeLoader(MakeAnim(3, posFrames: true)));
        var f = n.GetPosFrame(1.99);
        Assert.NotNull(f);
        Assert.Equal(1f, f!.Origin.Y); // frame index 1, not 2
    }

    [Fact]
    public void GetPosFrame_AnimWithoutPosFrames_ReturnsNull()
    {
        var n = new AnimSequenceNode();
        n.SetAnimationId(1, new FakeLoader(MakeAnim(3, posFrames: false)));
        Assert.Null(n.GetPosFrame(0));
    }

    [Fact]
    public void GetPartFrame_BoundsAndValue()
    {
        var n = new AnimSequenceNode();
        n.SetAnimationId(1, new FakeLoader(MakeAnim(3)));
        Assert.Null(n.GetPartFrame(-1));
        Assert.Null(n.GetPartFrame(3));
        var pf = n.GetPartFrame(2);
        Assert.NotNull(pf);
        Assert.Equal(2f, pf!.Frames[0].Origin.X);
    }

    [Fact]
    public void CtorFromAnimData_CopiesThenClamps()
    {
        QualifiedDataId<Animation> qid = 0x03001234u;
        var ad = new AnimData
        {
            AnimId = qid,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = 15f,
        };
        var n = new AnimSequenceNode(ad, new FakeLoader(MakeAnim(5)));
        Assert.Equal(15f, n.Framerate);
        Assert.Equal(0, n.LowFrame);
        Assert.Equal(4, n.HighFrame);
        Assert.True(n.HasAnim);
    }
}
