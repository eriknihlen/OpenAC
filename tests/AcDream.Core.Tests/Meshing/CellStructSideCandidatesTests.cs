using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.Core.Tests.Meshing;

public class CellStructSideCandidatesTests
{

    [Fact]
    public void SidesSingle_YieldsOnePositiveForwardCandidate()
    {
        var candidates = CellStructSideCandidates.GetCandidates(0).ToArray();

        var candidate = Assert.Single(candidates);
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, candidate.SurfaceSlot);
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, candidate.UvSlot);
        Assert.Equal(0, candidate.CopyOrdinal);
        Assert.Equal(1, candidate.NormalSign);
        Assert.False(candidate.ReverseWinding);
    }

    [Fact]
    public void SidesDouble_YieldsTwoPositiveCandidates_SecondCopyReversedWithNegativeNormal()
    {
        var candidates = CellStructSideCandidates.GetCandidates(1).ToArray();

        Assert.Equal(2, candidates.Length);

        var first = candidates[0];
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, first.SurfaceSlot);
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, first.UvSlot);
        Assert.Equal(0, first.CopyOrdinal);
        Assert.Equal(1, first.NormalSign);
        Assert.False(first.ReverseWinding);

        var second = candidates[1];
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, second.SurfaceSlot);
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, second.UvSlot);
        Assert.Equal(1, second.CopyOrdinal);
        Assert.Equal(-1, second.NormalSign);
        Assert.True(second.ReverseWinding);
    }

    [Fact]
    public void SidesBoth_YieldsPositiveThenNegativeCandidate_NegativeSideNotReversed()
    {
        var candidates = CellStructSideCandidates.GetCandidates(2).ToArray();

        Assert.Equal(2, candidates.Length);

        var positive = candidates[0];
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, positive.SurfaceSlot);
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, positive.UvSlot);
        Assert.Equal(0, positive.CopyOrdinal);
        Assert.Equal(1, positive.NormalSign);
        Assert.False(positive.ReverseWinding);

        var negative = candidates[1];
        Assert.Equal(CellStructPolygonSurfaceSide.Negative, negative.SurfaceSlot);
        Assert.Equal(CellStructPolygonSurfaceSide.Negative, negative.UvSlot);
        Assert.Equal(0, negative.CopyOrdinal);
        Assert.Equal(-1, negative.NormalSign);
        Assert.False(negative.ReverseWinding);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(99)]
    public void UnknownSidesValue_FallsBackToRetailSingleSideShape(int rawSidesType)
    {
        var candidates = CellStructSideCandidates.GetCandidates(rawSidesType);

        var single = Assert.Single(candidates.ToArray());
        Assert.Equal(CellStructPolygonSurfaceSide.Positive, single.SurfaceSlot);
        Assert.Equal(0, single.CopyOrdinal);
        Assert.Equal(1, single.NormalSign);
        Assert.False(single.ReverseWinding);
        Assert.False(CellStructSideCandidates.IsRetailDefinedSidesType(rawSidesType));
    }


    [Theory]
    [InlineData(0, 0, 1, 2)]
    [InlineData(1, 0, 2, 3)]
    [InlineData(5, 0, 6, 7)]
    public void ForwardWinding_ProducesForwardFanIndices(int triangleIndex, int a, int b, int c)
    {
        var (fa, fb, fc) = CellStructSideCandidates.TriangleFanIndices(triangleIndex, reverseWinding: false);

        Assert.Equal((a, b, c), (fa, fb, fc));
    }

    [Theory]
    [InlineData(0, 2, 1, 0)]
    [InlineData(1, 3, 2, 0)]
    [InlineData(5, 7, 6, 0)]
    public void ReversedWinding_ProducesReversedFanIndices(int triangleIndex, int a, int b, int c)
    {
        var (fa, fb, fc) = CellStructSideCandidates.TriangleFanIndices(triangleIndex, reverseWinding: true);

        Assert.Equal((a, b, c), (fa, fb, fc));
    }


    [Fact]
    public void NoPosStippling_MakesPositiveSlotCandidateUvAbsent_ButCandidateStillPresent()
    {
        var candidates = CellStructSideCandidates.GetCandidates(0).ToArray();
        var candidate = Assert.Single(candidates);

        Assert.True(CellStructSideCandidates.IsUvAbsent(candidate, StipplingType.NoPos));
    }

    [Fact]
    public void NoNegStippling_MakesNegativeSlotCandidateUvAbsent_ButCandidateStillPresent()
    {
        var candidates = CellStructSideCandidates.GetCandidates(2).ToArray();
        var negative = candidates[1];

        Assert.True(CellStructSideCandidates.IsUvAbsent(negative, StipplingType.NoNeg));
    }

    [Fact]
    public void NoNegStippling_DoesNotMakePositiveSlotCandidateUvAbsent()
    {
        var candidates = CellStructSideCandidates.GetCandidates(2).ToArray();
        var positive = candidates[0];

        Assert.False(CellStructSideCandidates.IsUvAbsent(positive, StipplingType.NoNeg));
    }

    [Fact]
    public void NoUvBits_LeavesUvPresent()
    {
        var candidates = CellStructSideCandidates.GetCandidates(0).ToArray();
        var candidate = Assert.Single(candidates);

        Assert.False(CellStructSideCandidates.IsUvAbsent(candidate, StipplingType.None));
    }

    // ---- §3.2 per-surface initial mask: exact precedence ----

    [Fact]
    public void ClipMapSurface_HasInitialMaskEight()
    {
        Assert.Equal(8, CellStructSideCandidates.InitialSurfaceMask(SurfaceType.Base1ClipMap));
    }

    [Fact]
    public void AlphaSurface_HasInitialMaskTwo()
    {
        Assert.Equal(2, CellStructSideCandidates.InitialSurfaceMask(SurfaceType.Alpha));
    }

    [Fact]
    public void InvAlphaSurface_HasInitialMaskTwo()
    {
        Assert.Equal(2, CellStructSideCandidates.InitialSurfaceMask(SurfaceType.InvAlpha));
    }

    [Fact]
    public void AdditiveSurface_HasInitialMaskTwo()
    {
        Assert.Equal(2, CellStructSideCandidates.InitialSurfaceMask(SurfaceType.Additive));
    }

    [Fact]
    public void TranslucentSurface_HasInitialMaskFour()
    {
        Assert.Equal(4, CellStructSideCandidates.InitialSurfaceMask(SurfaceType.Translucent));
    }

    [Fact]
    public void AlphaFamily_TakesPrecedenceOverClipMap()
    {
        var type = SurfaceType.Alpha | SurfaceType.Base1ClipMap;

        Assert.Equal(2, CellStructSideCandidates.InitialSurfaceMask(type));
    }

    [Fact]
    public void ClipMap_TakesPrecedenceOverTranslucent()
    {
        var type = SurfaceType.Base1ClipMap | SurfaceType.Translucent;

        Assert.Equal(8, CellStructSideCandidates.InitialSurfaceMask(type));
    }

    [Fact]
    public void PlainSolidSurface_HasInitialMaskZero()
    {
        Assert.Equal(0, CellStructSideCandidates.InitialSurfaceMask(SurfaceType.Base1Solid));
    }


    [Fact]
    public void UntexturedSolidSurfaceType_IsUntextured_ButCandidateIsStillConstructed()
    {
        // 0x1 = Base1Solid only.
        var type = (SurfaceType)0x1;
        Assert.True(RetailUntexturedSurfacePolicy.IsUntextured(type));

        var candidates = CellStructSideCandidates.GetCandidates(0);
        Assert.False(candidates.IsEmpty);
    }

    [Fact]
    public void UntexturedSolidTranslucentSurfaceType_IsUntextured_ButCandidateIsStillConstructed()
    {
        var type = (SurfaceType)0x11;
        Assert.True(RetailUntexturedSurfacePolicy.IsUntextured(type));
        Assert.Equal(4, CellStructSideCandidates.InitialSurfaceMask(type));

        var candidates = CellStructSideCandidates.GetCandidates(0);
        Assert.False(candidates.IsEmpty);
    }

    // ---- §3.2 per-polygon stippling mask update: signed-byte SETG,
    // aimed only at the positive surface ----

    [Fact]
    public void PositiveStippling_OrsBitOneOnThePositiveSurfaceCandidate()
    {
        var mask = CellStructSideCandidates.ApplyStipplingMaskBit(
            currentMask: 0,
            candidateSurfaceSlot: CellStructPolygonSurfaceSide.Positive,
            stippling: StipplingType.Positive);

        Assert.Equal(1, mask);
    }

    [Fact]
    public void NegativeStippling_LeavesTheNegativeSurfaceCandidateMaskUnchanged()
    {
        var mask = CellStructSideCandidates.ApplyStipplingMaskBit(
            currentMask: 4,
            candidateSurfaceSlot: CellStructPolygonSurfaceSide.Negative,
            stippling: StipplingType.Negative);

        Assert.Equal(4, mask);
    }

    [Fact]
    public void NoPosOrNoNegStippling_StillOrsBitOneOnThePositiveSurfaceCandidate()
    {
        var maskFromNoPos = CellStructSideCandidates.ApplyStipplingMaskBit(
            currentMask: 0,
            candidateSurfaceSlot: CellStructPolygonSurfaceSide.Positive,
            stippling: StipplingType.NoPos);
        var maskFromNoNeg = CellStructSideCandidates.ApplyStipplingMaskBit(
            currentMask: 0,
            candidateSurfaceSlot: CellStructPolygonSurfaceSide.Positive,
            stippling: StipplingType.NoNeg);

        Assert.Equal(1, maskFromNoPos);
        Assert.Equal(1, maskFromNoNeg);
    }

    [Fact]
    public void ZeroStippling_DoesNotOrBitOne()
    {
        var mask = CellStructSideCandidates.ApplyStipplingMaskBit(
            currentMask: 0,
            candidateSurfaceSlot: CellStructPolygonSurfaceSide.Positive,
            stippling: StipplingType.None);

        Assert.Equal(0, mask);
    }

    [Fact]
    public void CorruptRawStipplingValue_NegativeAsSignedByte_DoesNotOrBitOne()
    {
        var stippling = (StipplingType)0x80;

        var mask = CellStructSideCandidates.ApplyStipplingMaskBit(
            currentMask: 0,
            candidateSurfaceSlot: CellStructPolygonSurfaceSide.Positive,
            stippling: stippling);

        Assert.Equal(0, mask);
    }

    [Fact]
    public void PreservesUnrelatedMaskBits_WhenOringBitOne()
    {
        var mask = CellStructSideCandidates.ApplyStipplingMaskBit(
            currentMask: 8,
            candidateSurfaceSlot: CellStructPolygonSurfaceSide.Positive,
            stippling: StipplingType.Positive);

        Assert.Equal(9, mask);
    }
}
