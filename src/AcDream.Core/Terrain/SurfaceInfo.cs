namespace AcDream.Core.Terrain;

public readonly record struct SurfaceInfo(
    byte BaseLayer,
    byte Ovl0Layer, byte Ovl0AlphaLayer, byte Ovl0Rotation,
    byte Ovl1Layer, byte Ovl1AlphaLayer, byte Ovl1Rotation,
    byte Ovl2Layer, byte Ovl2AlphaLayer, byte Ovl2Rotation,
    byte RoadLayer, byte Road0AlphaLayer, byte Road0Rotation,
    byte Road1AlphaLayer, byte Road1Rotation)
{
    public const byte None = 255;

    /// <summary>An empty recipe where only the base is present and all overlays are None.</summary>
    public static SurfaceInfo BaseOnly(byte baseLayer) => new(
        baseLayer,
        None, None, 0, None, None, 0, None, None, 0,
        None, None, 0, None, 0);
}

public sealed record TerrainBlendingContext(
    IReadOnlyDictionary<uint, byte> TerrainTypeToLayer,
    byte RoadLayer,
    IReadOnlyList<byte> CornerAlphaLayers,
    IReadOnlyList<byte> SideAlphaLayers,
    IReadOnlyList<byte> RoadAlphaLayers,
    IReadOnlyList<uint> CornerAlphaTCodes,
    IReadOnlyList<uint> SideAlphaTCodes,
    IReadOnlyList<uint> RoadAlphaRCodes);
