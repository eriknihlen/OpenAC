namespace AcDream.App.Rendering;

internal interface IGameRenderResourceLifetime
{
    TerrainAtlas AcquireTerrainAtlas(Func<TerrainAtlas> factory);
}

/// <summary>
/// Sole lifetime owner for render resources that are borrowed by, but not
/// owned by, their renderers.
/// </summary>
internal sealed class GameRenderResourceLifetime : IGameRenderResourceLifetime
{
    private readonly OwnedResourceSlot<TerrainAtlas> _terrainAtlas = new();

    public TerrainAtlas AcquireTerrainAtlas(Func<TerrainAtlas> factory) =>
        _terrainAtlas.Acquire(factory);

    public void ReleaseTerrainAtlas() => _terrainAtlas.Release();
}
