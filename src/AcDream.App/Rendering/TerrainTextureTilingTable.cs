namespace AcDream.App.Rendering;

internal static class TerrainTextureTilingTable
{
    internal const int LayerCapacity = 36;

    internal const int UniformElementStrideBytes = 16;

    internal const int UniformBufferBytes = LayerCapacity * UniformElementStrideBytes;

    internal static float[] Build(IEnumerable<(uint Layer, uint RepeatCount)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var table = new float[LayerCapacity];
        Array.Fill(table, 1f);

        foreach (var (layer, repeatCount) in entries)
        {
            if (layer >= LayerCapacity)
            {
                throw new InvalidOperationException(
                    $"Terrain atlas layer {layer} exceeds the shader capacity of {LayerCapacity} layers.");
            }

            table[layer] = repeatCount;
        }

        return table;
    }
}
