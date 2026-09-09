using AcDream.Core.Rendering.Wb;
using DatReaderWriter.DBObjs;

namespace AcDream.Core.World;

internal static class WbSceneryAdapter
{
    private const int VerticesPerSide = 9;
    private const int TerrainSize     = VerticesPerSide * VerticesPerSide; // 81

    public static TerrainEntry[] BuildTerrainEntries(LandBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var entries = new TerrainEntry[TerrainSize];
        for (int i = 0; i < TerrainSize; i++)
        {
            var ti = block.Terrain[i];
            entries[i] = new TerrainEntry(
                height:     block.Height[i],
                texture:    (byte)ti.Type,
                scenery:    ti.Scenery,
                road:       ti.Road,
                encounters: null);
        }
        return entries;
    }
}
