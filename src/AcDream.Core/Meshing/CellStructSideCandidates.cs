using System;
using DatReaderWriter.Enums;

namespace AcDream.Core.Meshing;

public enum CellStructPolygonSurfaceSide
{
    /// <summary>Reads <c>pos_surface</c> and <c>pos_uv_indices</c>.</summary>
    Positive = 0,

    /// <summary>Reads <c>neg_surface</c> and <c>neg_uv_indices</c>.</summary>
    Negative = 1,
}

public readonly record struct CellStructSideCandidate(
    CellStructPolygonSurfaceSide SurfaceSlot,
    CellStructPolygonSurfaceSide UvSlot,
    int CopyOrdinal,
    int NormalSign,
    bool ReverseWinding);

public static class CellStructSideCandidates
{
    private static readonly CellStructSideCandidate[] SingleCandidates =
    {
        new(CellStructPolygonSurfaceSide.Positive, CellStructPolygonSurfaceSide.Positive, CopyOrdinal: 0, NormalSign: 1, ReverseWinding: false),
    };

    private static readonly CellStructSideCandidate[] DoubleCandidates =
    {
        new(CellStructPolygonSurfaceSide.Positive, CellStructPolygonSurfaceSide.Positive, CopyOrdinal: 0, NormalSign: 1, ReverseWinding: false),
        new(CellStructPolygonSurfaceSide.Positive, CellStructPolygonSurfaceSide.Positive, CopyOrdinal: 1, NormalSign: -1, ReverseWinding: true),
    };

    private static readonly CellStructSideCandidate[] BothCandidates =
    {
        new(CellStructPolygonSurfaceSide.Positive, CellStructPolygonSurfaceSide.Positive, CopyOrdinal: 0, NormalSign: 1, ReverseWinding: false),
        new(CellStructPolygonSurfaceSide.Negative, CellStructPolygonSurfaceSide.Negative, CopyOrdinal: 0, NormalSign: -1, ReverseWinding: false),
    };

    public static ReadOnlySpan<CellStructSideCandidate> GetCandidates(int rawSidesType) => rawSidesType switch
    {
        1 => DoubleCandidates,
        2 => BothCandidates,
        _ => SingleCandidates,
    };

    public static bool IsRetailDefinedSidesType(int rawSidesType) => rawSidesType is 0 or 1 or 2;

    public static (int A, int B, int C) TriangleFanIndices(int triangleIndex, bool reverseWinding) =>
        reverseWinding
            ? (triangleIndex + 2, triangleIndex + 1, 0)
            : (0, triangleIndex + 1, triangleIndex + 2);

    public static bool IsUvAbsent(CellStructSideCandidate candidate, StipplingType stippling)
    {
        var absenceBit = candidate.UvSlot == CellStructPolygonSurfaceSide.Positive
            ? StipplingType.NoPos
            : StipplingType.NoNeg;
        return (stippling & absenceBit) != 0;
    }

    private const SurfaceType AlphaFamilyMask =
        SurfaceType.Alpha | SurfaceType.InvAlpha | SurfaceType.Additive;

    public static int InitialSurfaceMask(SurfaceType type)
    {
        if ((type & AlphaFamilyMask) != 0) return 2;
        if ((type & SurfaceType.Base1ClipMap) != 0) return 8;
        if ((type & SurfaceType.Translucent) != 0) return 4;
        return 0;
    }

    public static int ApplyStipplingMaskBit(
        int currentMask,
        CellStructPolygonSurfaceSide candidateSurfaceSlot,
        StipplingType stippling)
    {
        if (candidateSurfaceSlot != CellStructPolygonSurfaceSide.Positive) return currentMask;

        var signedStippling = unchecked((sbyte)(byte)stippling);
        return signedStippling > 0 ? currentMask | 1 : currentMask;
    }
}
