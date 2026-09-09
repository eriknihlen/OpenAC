using AcDream.App.Rendering;
using AcDream.Core.Meshing;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailAlphaMeshRouterTests
{
    public static IEnumerable<object[]> RowBoundaryCases()
    {
        yield return new object[]
        {
            true, false, false, (byte)0x02, false,
            (int)RetailAlphaMeshAction.Immediate, (int)RetailAlphaList.Alpha, false,
        };

        yield return new object[]
        {
            false, true, true, (byte)0x08, false,
            (int)RetailAlphaMeshAction.Immediate, (int)RetailAlphaList.Alpha, false,
        };

        yield return new object[]
        {
            false, false, true, (byte)0x08, false,
            (int)RetailAlphaMeshAction.AppendClipAndImmediate, (int)RetailAlphaList.Clip, true,
        };

        yield return new object[]
        {
            false, false, true, (byte)0x09, false,
            (int)RetailAlphaMeshAction.AppendClipAndImmediate, (int)RetailAlphaList.Clip, true,
        };

        yield return new object[]
        {
            false, false, true, (byte)0x02, false,
            (int)RetailAlphaMeshAction.Append, (int)RetailAlphaList.Alpha, false,
        };

        yield return new object[]
        {
            false, false, false, (byte)0x02, false,
            (int)RetailAlphaMeshAction.Append, (int)RetailAlphaList.Alpha, false,
        };

        yield return new object[]
        {
            false, false, false, (byte)0x08, false,
            (int)RetailAlphaMeshAction.Append, (int)RetailAlphaList.Clip, false,
        };

        yield return new object[]
        {
            false, false, false, (byte)0x04, false,
            (int)RetailAlphaMeshAction.Append, (int)RetailAlphaList.Alpha, false,
        };

        yield return new object[]
        {
            false, false, false, (byte)0x01, true,
            (int)RetailAlphaMeshAction.Append, (int)RetailAlphaList.Alpha, false,
        };

        yield return new object[]
        {
            false, false, false, (byte)0x00, true,
            (int)RetailAlphaMeshAction.Append, (int)RetailAlphaList.Alpha, false,
        };

        yield return new object[]
        {
            false, false, false, (byte)0x00, false,
            (int)RetailAlphaMeshAction.Immediate, (int)RetailAlphaList.Alpha, false,
        };
    }

    [Theory]
    [MemberData(nameof(RowBoundaryCases))]
    public void Route_MatchesTheTracedBoundary(
        bool sky, bool detail, bool multipass, byte mask, bool hasAlpha,
        int expectedActionRaw, int expectedListRaw,
        bool expectedOverrideClipmap)
    {
        var expectedAction = (RetailAlphaMeshAction)expectedActionRaw;
        var expectedList = (RetailAlphaList)expectedListRaw;
        RetailAlphaMeshDecision decision = RetailAlphaMeshRouter.Route(
            sky, RetailAlphaMeshRouter.DefaultDelayMask, detail, multipass, mask, hasAlpha);

        Assert.Equal(expectedAction, decision.Action);
        if (expectedAction != RetailAlphaMeshAction.Immediate)
        {
            Assert.Equal(expectedList, decision.List);
            Assert.Equal(expectedOverrideClipmap, decision.OverrideClipmap);
        }
    }

    [Fact]
    public void Route_MatchesRestatedBranchTableAcrossEveryCell()
    {
        byte[] masks = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];
        bool[] bothBools = [false, true];
        int mismatches = 0;
        var firstMismatch = "";

        foreach (byte mask in masks)
        foreach (bool sky in bothBools)
        foreach (bool detail in bothBools)
        foreach (bool multipass in bothBools)
        foreach (bool hasAlpha in bothBools)
        {
            RetailAlphaMeshDecision actual = RetailAlphaMeshRouter.Route(
                sky, RetailAlphaMeshRouter.DefaultDelayMask, detail, multipass, mask, hasAlpha);
            RetailAlphaMeshDecision expected = RestatedBranchTableRoute(
                sky, RetailAlphaMeshRouter.DefaultDelayMask, detail, multipass, mask, hasAlpha);

            bool matches = actual.Action == expected.Action
                && (actual.Action == RetailAlphaMeshAction.Immediate
                    || (actual.List == expected.List
                        && actual.OverrideClipmap == expected.OverrideClipmap));
            if (!matches && mismatches++ == 0)
            {
                firstMismatch =
                    $"sky={sky} detail={detail} multipass={multipass} mask=0x{mask:x2} "
                    + $"hasAlpha={hasAlpha}: expected {expected}, got {actual}";
            }
        }

        Assert.True(mismatches == 0, $"{mismatches} mismatches; first: {firstMismatch}");
    }

    private static RetailAlphaMeshDecision RestatedBranchTableRoute(
        bool sky, byte delayMask, bool detail, bool multipass, byte mask, bool hasAlpha)
    {
        bool row1 = sky || delayMask == 0 || detail;
        if (row1)
            return new RetailAlphaMeshDecision(RetailAlphaMeshAction.Immediate, default, false);

        bool clipBit = (mask & 0b1000) == 0b1000;
        bool row2 = multipass switch
        {
            true => clipBit,
            false => false,
        };
        if (row2)
            return new RetailAlphaMeshDecision(RetailAlphaMeshAction.AppendClipAndImmediate, RetailAlphaList.Clip, true);

        int intersect = delayMask & mask;
        bool row3 = intersect != 0;
        if (row3)
        {
            RetailAlphaList list = clipBit ? RetailAlphaList.Clip : RetailAlphaList.Alpha;
            return new RetailAlphaMeshDecision(RetailAlphaMeshAction.Append, list, false);
        }

        bool delayHasTranslucentBit = (delayMask & 0b0100) == 0b0100;
        bool row4 = delayHasTranslucentBit && hasAlpha;
        if (row4)
            return new RetailAlphaMeshDecision(RetailAlphaMeshAction.Append, RetailAlphaList.Alpha, false);

        return new RetailAlphaMeshDecision(RetailAlphaMeshAction.Immediate, default, false);
    }

    [Theory]
    [InlineData(TranslucencyKind.AlphaBlend, RetailAlphaMeshRouter.MaskAlphaFamily)]
    [InlineData(TranslucencyKind.Additive, RetailAlphaMeshRouter.MaskAlphaFamily)]
    [InlineData(TranslucencyKind.InvAlpha, RetailAlphaMeshRouter.MaskAlphaFamily)]
    [InlineData(TranslucencyKind.ClipMap, RetailAlphaMeshRouter.MaskClipMap)]
    [InlineData(TranslucencyKind.Opaque, (byte)0)]
    public void MaskFromTranslucencyKind_MapsEveryKind(TranslucencyKind kind, byte expectedMask)
        => Assert.Equal(expectedMask, RetailAlphaMeshRouter.MaskFromTranslucencyKind(kind));

    [Fact]
    public void ConstructSubsetMask_AlphaFamilyWinsOverClipMapAndTranslucent()
    {
        byte mask = RetailAlphaMeshRouter.ConstructSubsetMask(
            hasAlphaFamilyBit: true, hasClipMapBit: true, hasTranslucentBit: true,
            hasPositiveStippling: false);
        Assert.Equal(RetailAlphaMeshRouter.MaskAlphaFamily, mask);
    }

    [Fact]
    public void ConstructSubsetMask_ClipMapWinsOverTranslucentWithoutAlphaFamily()
    {
        byte mask = RetailAlphaMeshRouter.ConstructSubsetMask(
            hasAlphaFamilyBit: false, hasClipMapBit: true, hasTranslucentBit: true,
            hasPositiveStippling: false);
        Assert.Equal(RetailAlphaMeshRouter.MaskClipMap, mask);
    }

    [Fact]
    public void ConstructSubsetMask_PositiveStipplingOrsIntoTheBaseMask()
    {
        byte mask = RetailAlphaMeshRouter.ConstructSubsetMask(
            hasAlphaFamilyBit: false, hasClipMapBit: false, hasTranslucentBit: true,
            hasPositiveStippling: true);
        Assert.Equal((byte)(RetailAlphaMeshRouter.MaskTranslucent | RetailAlphaMeshRouter.MaskPositiveStipple), mask);
    }
}
