using AcDream.Core.Meshing;

namespace AcDream.App.Rendering;

/// <summary>What a routed subset does with the two alpha lists.</summary>
internal enum RetailAlphaMeshAction
{
    /// <summary>Rows 1/5 — draw now; never touches a list.</summary>
    Immediate,

    /// <summary>Rows 3/4 — append only; no immediate draw this frame.</summary>
    Append,

    AppendClipAndImmediate,
}

internal readonly record struct RetailAlphaMeshDecision(
    RetailAlphaMeshAction Action,
    RetailAlphaList List,
    bool OverrideClipmap);

internal static class RetailAlphaMeshRouter
{
    internal const byte MaskAlphaFamily = 0x02;

    internal const byte MaskTranslucent = 0x04;

    internal const byte MaskClipMap = 0x08;

    internal const byte MaskPositiveStipple = 0x01;

    internal const byte DefaultDelayMask = 0x0E;

    internal static byte ConstructSubsetMask(
        bool hasAlphaFamilyBit,
        bool hasClipMapBit,
        bool hasTranslucentBit,
        bool hasPositiveStippling)
    {
        byte mask = hasAlphaFamilyBit
            ? MaskAlphaFamily
            : hasClipMapBit
                ? MaskClipMap
                : hasTranslucentBit
                    ? MaskTranslucent
                    : (byte)0;
        if (hasPositiveStippling)
            mask |= MaskPositiveStipple;
        return mask;
    }

    internal static byte MaskFromTranslucencyKind(TranslucencyKind kind) => kind switch
    {
        TranslucencyKind.ClipMap => MaskClipMap,
        TranslucencyKind.Opaque => 0,
        _ => MaskAlphaFamily, // AlphaBlend, Additive, InvAlpha
    };

    internal static RetailAlphaMeshDecision Route(
        bool currentlyDrawingSky,
        byte delayMask,
        bool detailSurfaceActive,
        bool multiPassAlpha,
        byte subsetMask,
        bool materialHasAlpha)
    {
        if (currentlyDrawingSky || delayMask == 0 || detailSurfaceActive)
            return new RetailAlphaMeshDecision(RetailAlphaMeshAction.Immediate, default, false);

        bool clipMapBitSet = (subsetMask & MaskClipMap) != 0;

        // Row 2: MultiPassAlpha && (mask & 0x08) != 0
        if (multiPassAlpha && clipMapBitSet)
        {
            return new RetailAlphaMeshDecision(
                RetailAlphaMeshAction.AppendClipAndImmediate, RetailAlphaList.Clip, true);
        }

        if ((delayMask & subsetMask) != 0)
        {
            return new RetailAlphaMeshDecision(
                RetailAlphaMeshAction.Append,
                clipMapBitSet ? RetailAlphaList.Clip : RetailAlphaList.Alpha,
                false);
        }

        // Row 4: (delayMask & 0x04) != 0 && material != null && material.has_alpha != 0
        if ((delayMask & MaskTranslucent) != 0 && materialHasAlpha)
            return new RetailAlphaMeshDecision(RetailAlphaMeshAction.Append, RetailAlphaList.Alpha, false);

        // Row 5: otherwise
        return new RetailAlphaMeshDecision(RetailAlphaMeshAction.Immediate, default, false);
    }
}
