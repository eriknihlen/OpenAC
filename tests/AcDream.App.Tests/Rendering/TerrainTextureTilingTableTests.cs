using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class TerrainTextureTilingTableTests
{
    [Fact]
    public void Build_MapsDatRepeatCountsToAtlasLayers()
    {
        var table = TerrainTextureTilingTable.Build(
        [
            (Layer: 0u, RepeatCount: 4u),
            (Layer: 7u, RepeatCount: 2u),
            (Layer: 32u, RepeatCount: 8u),
        ]);

        Assert.Equal(TerrainTextureTilingTable.LayerCapacity, table.Length);
        Assert.Equal(4f, table[0]);
        Assert.Equal(2f, table[7]);
        Assert.Equal(8f, table[32]);
    }

    [Fact]
    public void Build_DefaultsOnlyUnusedLayersToOne()
    {
        var table = TerrainTextureTilingTable.Build(
        [
            (Layer: 3u, RepeatCount: 0u),
        ]);

        Assert.Equal(1f, table[2]);
        Assert.Equal(0f, table[3]);
        Assert.Equal(1f, table[4]);
    }

    [Fact]
    public void Build_RejectsLayersTheShaderCannotAddress()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TerrainTextureTilingTable.Build(
            [
                (Layer: (uint)TerrainTextureTilingTable.LayerCapacity, RepeatCount: 1u),
            ]));

        Assert.Contains("exceeds the shader capacity", ex.Message);
    }

    [Fact]
    public void ModernShader_AppliesTheOwningLayersRepeatCountToEveryTerrainSample()
    {
        string shaderPath = Path.Combine(
            AppContext.BaseDirectory,
            "Rendering",
            "Shaders",
            "terrain_modern.frag");
        string shader = File.ReadAllText(shaderPath);

        Assert.Contains("binding = 3) uniform TerrainTiling {", shader);
        Assert.Contains("float uTexTiling[36];", shader);
        Assert.Contains("baseUV * terrainTiling(pOverlay0.z)", shader);
        Assert.Contains("baseUV * terrainTiling(pOverlay1.z)", shader);
        Assert.Contains("baseUV * terrainTiling(pOverlay2.z)", shader);
        Assert.Contains("baseUV * terrainTiling(pRoad0.z)", shader);
        Assert.Contains("vBaseUV * terrainTiling(vBaseTexIdx)", shader);
        Assert.DoesNotContain("const float TILE", shader);
    }

    [Fact]
    public void UniformBufferMatchesTheStd140LayoutTheShaderDeclares()
    {
        Assert.Equal(16, TerrainTextureTilingTable.UniformElementStrideBytes);
        Assert.Equal(576, TerrainTextureTilingTable.UniformBufferBytes);
        Assert.Equal(
            TerrainTextureTilingTable.LayerCapacity
                * TerrainTextureTilingTable.UniformElementStrideBytes,
            TerrainTextureTilingTable.UniformBufferBytes);

        // A std140 float array element is padded, never packed. If this ever
        // equals sizeof(float) the writer above has been "simplified" into the
        // bug this test exists for.
        Assert.NotEqual(sizeof(float), TerrainTextureTilingTable.UniformElementStrideBytes);
    }
}
