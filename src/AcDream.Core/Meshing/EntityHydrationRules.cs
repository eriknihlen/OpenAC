namespace AcDream.Core.Meshing;

public static class EntityHydrationRules
{
    /// <summary>
    /// True when the entity should still be added to the landblock's entity
    /// set even with zero mesh refs, because it has dat-authored lights to
    /// register. An entity with any mesh is always kept (unchanged from the
    /// pre-existing gate); the entity is dropped only when it has neither
    /// geometry to draw nor lights to register.
    /// </summary>
    public static bool ShouldKeepEntity(int meshRefCount, int setupLightCount)
        => meshRefCount > 0 || setupLightCount > 0;
}
