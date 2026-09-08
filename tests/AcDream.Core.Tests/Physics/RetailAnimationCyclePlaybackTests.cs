using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class RetailAnimationCyclePlaybackTests
{
    private static Animation MakeAnim(int numFrames, int numParts, Vector3 origin, Quaternion orientation)
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

    [Fact]
    public void Advance_WithinSpan_AddsElapsedTimesFramerate()
    {
        float result = RetailAnimationCyclePlayback.Advance(
            currFrame: 5f, lowFrame: 0, highFrame: 29, framerate: 30f, elapsedSeconds: 0.1f);

        Assert.Equal(8f, result, precision: 4); // 5 + 0.1*30 = 8.
    }

    [Fact]
    public void Advance_PastHighFrame_WrapsBackToLowFrame()
    {
        float result = RetailAnimationCyclePlayback.Advance(
            currFrame: 25f, lowFrame: 0, highFrame: 29, framerate: 30f, elapsedSeconds: 0.2f);

        // 25 + 6 = 31, over highFrame(29) by span+1=30: over = 31-0 = 31,
        // wrapped = 0 + (31 % 30) = 1.
        Assert.Equal(1f, result, precision: 4);
    }

    [Fact]
    public void Advance_BelowLowFrame_ClampsToLowFrame()
    {
        float result = RetailAnimationCyclePlayback.Advance(
            currFrame: -5f, lowFrame: 0, highFrame: 29, framerate: 30f, elapsedSeconds: 0.05f);

        Assert.Equal(0f, result);
    }

    [Theory]
    [InlineData(0, 0)]   // degenerate span (highFrame == lowFrame).
    [InlineData(0, -1)]  // inverted span.
    public void Advance_DegenerateSpan_ReturnsCurrFrameUnchanged(int lowFrame, int highFrame)
    {
        float result = RetailAnimationCyclePlayback.Advance(
            currFrame: 3f, lowFrame, highFrame, framerate: 30f, elapsedSeconds: 1f);

        Assert.Equal(3f, result);
    }

    [Fact]
    public void Advance_ZeroFramerateOrNonPositiveElapsed_ReturnsCurrFrameUnchanged()
    {
        Assert.Equal(3f, RetailAnimationCyclePlayback.Advance(3f, 0, 29, framerate: 0f, elapsedSeconds: 1f));
        Assert.Equal(3f, RetailAnimationCyclePlayback.Advance(3f, 0, 29, framerate: 30f, elapsedSeconds: 0f));
        Assert.Equal(3f, RetailAnimationCyclePlayback.Advance(3f, 0, 29, framerate: 30f, elapsedSeconds: -1f));
    }

    [Fact]
    public void Advance_NegativeFramerate_MovesBackwardAndClampsAtLowFrame()
    {
        Assert.Equal(3f, RetailAnimationCyclePlayback.Advance(
            5f, lowFrame: 0, highFrame: 29, framerate: -2f, elapsedSeconds: 1f));
        Assert.Equal(0f, RetailAnimationCyclePlayback.Advance(
            1f, lowFrame: 0, highFrame: 29, framerate: -2f, elapsedSeconds: 1f));
    }

    [Fact]
    public void TryInterpolatePart_ExactFrame_ReturnsThatFramesPose()
    {
        Animation anim = MakeAnim(3, 2, new Vector3(1f, 2f, 3f), Quaternion.Identity);

        bool ok = RetailAnimationCyclePlayback.TryInterpolatePart(
            anim, currFrame: 1f, lowFrame: 0, highFrame: 2, partIndex: 0,
            out Vector3 origin, out Quaternion orientation);

        Assert.True(ok);
        Assert.Equal(new Vector3(1f, 2f, 3f), origin);
        Assert.Equal(Quaternion.Identity, orientation);
    }

    [Fact]
    public void TryInterpolatePart_BetweenFrames_LerpsOriginHalfway()
    {
        var anim = new Animation();
        var pf0 = new AnimationFrame(1);
        pf0.Frames.Add(new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity });
        var pf1 = new AnimationFrame(1);
        pf1.Frames.Add(new Frame { Origin = new Vector3(10f, 0f, 0f), Orientation = Quaternion.Identity });
        anim.PartFrames.Add(pf0);
        anim.PartFrames.Add(pf1);

        bool ok = RetailAnimationCyclePlayback.TryInterpolatePart(
            anim, currFrame: 0.5f, lowFrame: 0, highFrame: 1, partIndex: 0,
            out Vector3 origin, out _);

        Assert.True(ok);
        Assert.Equal(new Vector3(5f, 0f, 0f), origin);
    }

    [Fact]
    public void TryInterpolatePart_AtHighFrame_WrapsNextFrameToLowFrame()
    {
        var anim = new Animation();
        var pf0 = new AnimationFrame(1);
        pf0.Frames.Add(new Frame { Origin = new Vector3(1f, 0f, 0f), Orientation = Quaternion.Identity });
        var pf1 = new AnimationFrame(1);
        pf1.Frames.Add(new Frame { Origin = new Vector3(2f, 0f, 0f), Orientation = Quaternion.Identity });
        anim.PartFrames.Add(pf0);
        anim.PartFrames.Add(pf1);

        bool ok = RetailAnimationCyclePlayback.TryInterpolatePart(
            anim, currFrame: 1f, lowFrame: 0, highFrame: 1, partIndex: 0,
            out Vector3 origin, out _);

        Assert.True(ok);
        Assert.Equal(new Vector3(2f, 0f, 0f), origin);
    }

    [Fact]
    public void TryInterpolatePart_PartIndexOutOfRange_ReturnsFalse()
    {
        Animation anim = MakeAnim(2, 1, Vector3.Zero, Quaternion.Identity);

        bool ok = RetailAnimationCyclePlayback.TryInterpolatePart(
            anim, currFrame: 0f, lowFrame: 0, highFrame: 1, partIndex: 5,
            out Vector3 origin, out Quaternion orientation);

        Assert.False(ok);
        Assert.Equal(default(Vector3), origin);
        Assert.Equal(default(Quaternion), orientation);
    }
}
